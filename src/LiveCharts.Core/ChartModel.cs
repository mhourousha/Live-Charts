﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using LiveCharts.Core.Abstractions;
using LiveCharts.Core.Data;
using LiveCharts.Core.Dimensions;
using Size = LiveCharts.Core.Drawing.Size;

namespace LiveCharts.Core
{
    /// <summary>
    /// Defines a chart.
    /// </summary>
    public abstract class ChartModel : IDisposable
    {
        private static int _colorCount;
        private Task _delayer;
        private readonly Dictionary<string, object> _propertyReferences = new Dictionary<string, object>();
        private object _updateId;
        private IList<Color> _colors;
        private readonly Dictionary<IDisposable, object> _resources =
            new Dictionary<IDisposable, object>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ChartModel"/> class.
        /// </summary>
        /// <param name="view">The chart view.</param>
        protected ChartModel(IChartView view)
        {
            View = view;
            view.ChartViewInitialized += ChartViewOnInitialized;
            view.UpdaterFrequencyChanged += ChartViewOnUpdaterFreqChanged;
            view.PropertyInstanceChanged += ChartViewOnPropertyInstanceChanged;
        }

        /// <summary>
        /// Gets the update identifier.
        /// </summary>
        /// <value>
        /// The update identifier.
        /// </value>
        public object UpdateId
        {
            get
            {
                lock (_updateId)
                {
                    return _updateId;
                }
            }
            private set
            {
                lock (_updateId)
                {
                    _updateId = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets the size of the draw area.
        /// </summary>
        /// <value>
        /// The size of the draw area.
        /// </value>
        public Size DrawAreaSize { get; set; }

        /// <summary>
        /// Gets the data range matrix.
        /// </summary>
        /// <value>
        /// The data range matrix.
        /// </value>
        public DimensionRange[][] DataRangeMatrix { get; protected set; }

        /// <summary>
        /// Gets the chart view.
        /// </summary>
        /// <value>
        /// The chart view.
        /// </value>
        public IChartView View { get; }

        /// <summary>
        /// Gets a value indicating whether this instance view is initialized.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance view is initialized; otherwise, <c>false</c>.
        /// </value>
        public bool IsViewInitialized { get; private set; }

        /// <summary>
        /// Gets or sets the colors.
        /// </summary>
        /// <value>
        /// The colors.
        /// </value>
        public IList<Color> Colors
        {
            get => _colors ?? LiveCharts.Options.Colors;
            set => _colors = value;
        }
        
        /// <summary>
        /// Invalidates this instance, the chart will queue an update request.
        /// </summary>
        /// <returns></returns>
        public async void Invalidate()
        {
            if (_delayer == null || !_delayer.IsCompleted) return;
            var delay = View.DisableAnimations
                ? TimeSpan.FromMilliseconds(10)
                : View.AnimationsSpeed;
            _delayer = Task.Delay(delay);
            await _delayer;
            Update(false);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            View.ChartViewInitialized -= ChartViewOnInitialized;
            View.UpdaterFrequencyChanged -= ChartViewOnUpdaterFreqChanged;
            foreach (var reference in _propertyReferences)
            {
                if (reference.Value is INotifyCollectionChanged incc)
                {
                    incc.CollectionChanged -= OnCollectionChangedUpdate;
                }
            }
        }

        /// <summary>
        /// Scales a number according to an axis range, to a given area, if the area is not present, the chart draw margin size will be used.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="plane">The axis.</param>
        /// <param name="size">The draw margin, this param is optional, if not set, the current chart's draw margin area will be used.</param>
        /// <returns></returns>
        public double ScaleTo(double value, Plane plane, Size? size = null)
        {
            var chartSize = size ?? DrawAreaSize;

            // based on the linear equation 
            // y = m * (x - x1) + y1
            // where m is the slope, (y2 - y1) / (x2 - x1)

            // for now we only support cartesian planes, X, other wise we suppose it is Y.
            var dimension = plane.Type == PlaneTypes.X ? chartSize.Width : chartSize.Height;

            double x1 = plane.ActualMaxValue, y1 = dimension;
            double x2 = plane.ActualMinValue; // y2 = 0;

            // m was simplified from => ((0 - y1) / (x2-x1))
            return (y1 / (x1 - x2)) * (value - x1) + y1;
        }

        /// <summary>
        /// Updates the chart.
        /// </summary>
        /// <param name="restart">if set to <c>true</c> all the elements in the view will be redrawn.</param>
        /// <exception cref="NotImplementedException"></exception>
        protected virtual void Update(bool restart)
        {
            UpdateId = new object();

            if (!IsViewInitialized)
            {
                Invalidate();
                return;
            }

            if (restart)
            {
                foreach (var resource in _resources)
                {
                    resource.Key.Dispose();
                }
                _resources.Clear();
            }

            // [ x: [x1: range, x2: range, x3: range, ..., xn: range], y: [...], z[...], w[...] ]
            DataRangeMatrix = View.AxisArrayByDimension.Select(
                    x => x.Select(
                            y => new DimensionRange(
                                double.IsNaN(y.MinValue) ? double.PositiveInfinity : y.MinValue,
                                double.IsNaN(y.MaxValue) ? double.NegativeInfinity : y.MaxValue))
                        .ToArray())
                .ToArray();

            foreach (var series in View.Series.Where(x => x.IsVisible))
            {
                series.FetchData(this);
                RegisterResource(series);
            }
        }

        internal Color GetNextColor()
        {
            return Colors[_colorCount++ % Colors.Count];
        }

        internal void RegisterResource(IDisposable disposable)
        {
            if (!_resources.ContainsKey(disposable))
            {
                _resources.Add(disposable, UpdateId);
                return;
            }
            _resources[disposable] = UpdateId;
        }

        internal void CollectResources()
        {
            foreach (var disposable in _resources.ToArray())
            {
                if (disposable.Value == UpdateId) continue;
                disposable.Key.Dispose();
                _resources.Remove(disposable.Key);
            }
        }

        private void ChartViewOnInitialized()
        {
            IsViewInitialized = true;
            Update(false);
        }

        private void ChartViewOnPropertyInstanceChanged(object instance, string propertyName)
        {
            if (_propertyReferences.TryGetValue(propertyName, out object previousInstance))
            {
                if (previousInstance is INotifyCollectionChanged previousIncc)
                {
                    previousIncc.CollectionChanged -= OnCollectionChangedUpdate;
                }
            }
            if (instance is INotifyCollectionChanged incc)
            {
                incc.CollectionChanged += OnCollectionChangedUpdate;
            }
            _propertyReferences[propertyName] = instance;
        }

        private void OnCollectionChangedUpdate(object sender, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs)
        {
            Invalidate();
        }

        private void ChartViewOnUpdaterFreqChanged(TimeSpan newValue)
        {
            Invalidate();
        }
    }
}